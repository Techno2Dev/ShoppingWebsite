import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { environment } from '../environments/environment';

export interface Product {
  id: number;
  name: string;
  description: string;
  price: number;
  image: string;
  imageUrl?: string | null;
  category: string;
  stock: number;
}

export interface CartLine {
  product: Product;
  quantity: number;
}

export interface OrderItem {
  id: number;
  orderId: number;
  productId: number;
  productName: string;
  quantity: number;
  unitPrice: number;
}

export interface StoreOrder {
  id: number;
  customerName: string;
  email: string;
  address: string;
  paymentStatus: string;
  createdAt: string;
  total: number;
  items: OrderItem[];
}

export interface AdminSummary {
  totalOrders: number;
  revenue: number;
  pendingPayments: number;
  productCount: number;
}

@Injectable({ providedIn: 'root' })
export class StoreApiService {
  private readonly apiUrl = environment.apiUrl;

  constructor(private http: HttpClient) { }

  getProducts(): Observable<Product[]> {
    const apiOrigin = new URL(this.apiUrl).origin;
    return this.http.get<Product[]>(this.apiUrl + '/products').pipe(map(products =>
      products.map(product => ({
        ...product,
        imageUrl: product.imageUrl ? new URL(product.imageUrl, apiOrigin).toString() : null
      }))
    ));
  }

  createProduct(product: Omit<Product, 'id' | 'imageUrl'>, imageFile?: File): Observable<Product> {
    const formData = new FormData();
    formData.append('name', product.name);
    formData.append('description', product.description);
    formData.append('price', String(product.price));
    formData.append('image', product.image);
    formData.append('category', product.category);
    formData.append('stock', String(product.stock));
    if (imageFile) {
      formData.append('imageFile', imageFile, imageFile.name);
    }
    return this.http.post<Product>(this.apiUrl + '/admin/products', formData, { withCredentials: true });
  }

  deleteProduct(productId: number): Observable<void> {
    return this.http.delete<void>(this.apiUrl + '/admin/products/' + productId, { withCredentials: true });
  }

  getOrders(): Observable<StoreOrder[]> {
    return this.http.get<StoreOrder[]>(this.apiUrl + '/orders', { withCredentials: true });
  }

  getSummary(): Observable<AdminSummary> {
    return this.http.get<AdminSummary>(this.apiUrl + '/admin/summary', { withCredentials: true });
  }

  adminLogin(userName: string, password: string): Observable<{ userName: string }> {
    return this.http.post<{ userName: string }>(
      this.apiUrl + '/admin/auth/login',
      { userName: userName, password: password },
      { withCredentials: true }
    );
  }

  getAdminSession(): Observable<{ userName: string }> {
    return this.http.get<{ userName: string }>(this.apiUrl + '/admin/auth/session', { withCredentials: true });
  }

  updateAdminCredentials(credentials: {
    currentPassword: string;
    newUserName: string;
    newPassword: string;
  }): Observable<{ userName: string }> {
    return this.http.post<{ userName: string }>(
      this.apiUrl + '/admin/auth/credentials',
      credentials,
      { withCredentials: true }
    );
  }

  adminLogout(): Observable<void> {
    return this.http.post<void>(this.apiUrl + '/admin/auth/logout', {}, { withCredentials: true });
  }

  createOrder(order: {
    customerName: string;
    email: string;
    address: string;
    items: { productId: number; quantity: number }[];
  }): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(this.apiUrl + '/orders', order);
  }

  updatePaymentStatus(orderId: number, paymentStatus: string): Observable<StoreOrder> {
    return this.http.post<StoreOrder>(
      this.apiUrl + '/admin/orders/' + orderId + '/payment-status',
      { paymentStatus: paymentStatus },
      { withCredentials: true }
    );
  }
}
